using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ArmaAiBridge.App.Services;

public sealed class AssemblyAiSpeechToTextService : ISpeechToTextService, IDisposable
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _pollTimeout;

    public AssemblyAiSpeechToTextService()
        : this(new HttpClient
        {
            BaseAddress = new Uri("https://api.assemblyai.com/v2/"),
            Timeout = TimeSpan.FromSeconds(30)
        }, ownsHttpClient: true)
    {
    }

    public AssemblyAiSpeechToTextService(
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? pollTimeout = null)
        : this(httpClient, ownsHttpClient: false, delay, pollTimeout)
    {
    }

    private AssemblyAiSpeechToTextService(
        HttpClient httpClient,
        bool ownsHttpClient,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? pollTimeout = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _delay = delay ?? Task.Delay;
        _pollTimeout = pollTimeout ?? PollTimeout;
        if (_pollTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollTimeout));
    }

    public async Task<string> TranscribeAsync(
        IAudioRecording recording,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw Failure("Save an AssemblyAI API key first.", "credentials");

        string key = apiKey.Trim();
        string uploadUrl = await UploadAsync(recording, key, cancellationToken).ConfigureAwait(false);
        string transcriptId = await CreateTranscriptAsync(uploadUrl, key, cancellationToken).ConfigureAwait(false);

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_pollTimeout);
        try
        {
            while (true)
            {
                using JsonDocument response = await SendJsonAsync(
                    HttpMethod.Get,
                    $"transcript/{Uri.EscapeDataString(transcriptId)}",
                    key,
                    content: null,
                    "poll",
                    deadline.Token).ConfigureAwait(false);
                JsonElement root = response.RootElement;
                string status = ReadString(root, "status");
                if (status == "completed")
                {
                    string transcript = ReadString(root, "text").Trim();
                    if (transcript.Length == 0)
                        throw Failure("AssemblyAI returned no speech. Check the microphone and try again.", "poll");
                    return transcript;
                }

                if (status == "error")
                    throw Failure("AssemblyAI could not transcribe the recording. Check the audio and try again.", "poll");
                if (status is not ("queued" or "processing"))
                    throw Failure("AssemblyAI returned an invalid transcription status.", "poll");

                await _delay(PollInterval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("AssemblyAI transcription timed out. Try again.", "poll_timeout");
        }
    }

    private async Task<string> UploadAsync(
        IAudioRecording recording,
        string apiKey,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await recording.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using StreamContent content = new(stream);
        content.Headers.ContentType = new("application/octet-stream");
        using JsonDocument response = await SendJsonAsync(
            HttpMethod.Post, "upload", apiKey, content, "upload", cancellationToken).ConfigureAwait(false);
        string uploadUrl = ReadString(response.RootElement, "upload_url");
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out _))
            throw Failure("AssemblyAI returned an invalid upload response.", "upload_parse");
        return uploadUrl;
    }

    private async Task<string> CreateTranscriptAsync(
        string uploadUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(new { audio_url = uploadUrl, language_detection = true });
        using StringContent content = new(body, Encoding.UTF8, "application/json");
        using JsonDocument response = await SendJsonAsync(
            HttpMethod.Post, "transcript", apiKey, content, "create", cancellationToken).ConfigureAwait(false);
        string transcriptId = ReadString(response.RootElement, "id");
        if (string.IsNullOrWhiteSpace(transcriptId))
            throw Failure("AssemblyAI returned an invalid transcript response.", "create_parse");
        return transcriptId;
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string path,
        string apiKey,
        HttpContent? content,
        string stage,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", apiKey);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("AssemblyAI did not respond in time. Try again.", $"{stage}_timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw Failure("Could not reach AssemblyAI. Check the network and try again.", stage, inner: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw HttpFailure(response.StatusCode, stage);
            byte[] bytes = await ReadBoundedAsync(response.Content, MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonDocument.Parse(bytes);
            }
            catch (JsonException exception)
            {
                throw Failure("AssemblyAI returned an invalid response. Try again.", $"{stage}_parse", inner: exception);
            }
        }
    }

    private static SpeechServiceException HttpFailure(HttpStatusCode status, string stage)
    {
        int code = (int)status;
        string message = code switch
        {
            401 or 403 => "AssemblyAI rejected the API key. Save a valid key and try again.",
            429 => "AssemblyAI rate-limited the request. Wait and try again.",
            >= 500 => "AssemblyAI is temporarily unavailable. Try again later.",
            _ => "AssemblyAI rejected the recording. Check the audio and settings."
        };
        return Failure(message, stage, code);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maximumBytes)
                throw Failure("AssemblyAI returned an oversized response.", "response_limit");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static SpeechServiceException Failure(
        string message,
        string stage,
        int? status = null,
        Exception? inner = null)
        => new(message, "assemblyai", stage, status, inner);

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
