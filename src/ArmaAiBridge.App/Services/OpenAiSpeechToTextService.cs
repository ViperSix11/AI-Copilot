using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ArmaAiBridge.App.Services;

public sealed class OpenAiSpeechToTextService : ISpeechToTextService, IDisposable
{
    internal const string TranscriptionModel = "gpt-4o-mini-transcribe";
    private const int MaximumAudioBytes = 600 * 1024;
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _requestTimeout;

    public OpenAiSpeechToTextService()
        : this(new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/v1/"),
            Timeout = TimeSpan.FromSeconds(60)
        }, ownsHttpClient: true, DefaultRequestTimeout)
    {
    }

    public OpenAiSpeechToTextService(HttpClient httpClient, TimeSpan? requestTimeout = null)
        : this(httpClient, ownsHttpClient: false, requestTimeout ?? DefaultRequestTimeout)
    {
    }

    private OpenAiSpeechToTextService(HttpClient httpClient, bool ownsHttpClient, TimeSpan requestTimeout)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        if (requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _requestTimeout = requestTimeout;
    }

    public async Task<string> TranscribeAsync(
        IAudioRecording recording,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recording);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw Failure("Save an OpenAI API key first.", "credentials");

        byte[] audio = await ReadAudioAsync(recording, cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            using MultipartFormDataContent content = new();
            using ByteArrayContent file = new(audio);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(file, "file", "capture.wav");
            content.Add(new StringContent(TranscriptionModel), "model");
            content.Add(new StringContent("de"), "language");
            content.Add(new StringContent("json"), "response_format");

            using HttpRequestMessage request = new(HttpMethod.Post, "audio/transcriptions") { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Failure("OpenAI transcription did not respond in time. Try again.", "timeout");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                throw Failure("Could not reach OpenAI transcription. Check the network and try again.", "network", inner: exception);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw HttpFailure(response.StatusCode);

                byte[] bytes = await ReadResponseAsync(response.Content, deadline.Token).ConfigureAwait(false);
                try
                {
                    using JsonDocument document = JsonDocument.Parse(bytes);
                    string transcript = document.RootElement.ValueKind == JsonValueKind.Object &&
                                        document.RootElement.TryGetProperty("text", out JsonElement text) &&
                                        text.ValueKind == JsonValueKind.String
                        ? (text.GetString() ?? string.Empty).Trim()
                        : string.Empty;
                    if (transcript.Length == 0)
                        throw Failure("OpenAI transcription returned no speech. Check the microphone and try again.", "empty");
                    return transcript;
                }
                catch (JsonException exception)
                {
                    throw Failure("OpenAI transcription returned an invalid response. Try again.", "parse", inner: exception);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("OpenAI transcription did not respond in time. Try again.", "timeout");
        }
        catch (HttpRequestException exception)
        {
            throw Failure("Could not reach OpenAI transcription. Check the network and try again.", "network", inner: exception);
        }
        catch (IOException exception)
        {
            throw Failure("Could not read the OpenAI transcription response. Check the network and try again.", "network", inner: exception);
        }
        finally
        {
            Array.Clear(audio);
        }
    }

    private static async Task<byte[]> ReadAudioAsync(
        IAudioRecording recording,
        CancellationToken cancellationToken)
    {
        await using Stream input = await recording.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > MaximumAudioBytes)
                throw Failure("The microphone recording is too large. Record a shorter message and try again.", "audio_limit");
            output.Write(buffer, 0, count);
        }

        if (output.Length == 0)
            throw Failure("The microphone recording is empty. Record a message and try again.", "audio_empty");
        return output.ToArray();
    }

    private static async Task<byte[]> ReadResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > MaximumResponseBytes)
                throw Failure("OpenAI transcription returned an oversized response.", "response_limit");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static SpeechServiceException HttpFailure(HttpStatusCode status)
    {
        int code = (int)status;
        string message = code switch
        {
            401 or 403 => "OpenAI transcription rejected the API key. Save a valid key and try again.",
            429 => "OpenAI transcription rate-limited the request. Wait and try again.",
            >= 500 => "OpenAI transcription is temporarily unavailable. Try again later.",
            _ => "OpenAI transcription rejected the recording. Check the audio and settings."
        };
        return Failure(message, "http", code);
    }

    private static SpeechServiceException Failure(
        string message,
        string stage,
        int? status = null,
        Exception? inner = null)
        => new(message, "openai-transcription", stage, status, inner);

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
