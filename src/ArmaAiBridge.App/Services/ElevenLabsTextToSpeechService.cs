using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ArmaAiBridge.App.Services;

public sealed class ElevenLabsTextToSpeechService : ITextToSpeechService, IDisposable
{
    public const int MaximumAudioBytes = 10 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public ElevenLabsTextToSpeechService()
        : this(new HttpClient
        {
            BaseAddress = new Uri("https://api.elevenlabs.io/v1/"),
            Timeout = TimeSpan.FromSeconds(60)
        }, ownsHttpClient: true)
    {
    }

    public ElevenLabsTextToSpeechService(HttpClient httpClient)
        : this(httpClient, ownsHttpClient: false)
    {
    }

    private ElevenLabsTextToSpeechService(HttpClient httpClient, bool ownsHttpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<AudioPayload> SynthesizeAsync(
        string text,
        string apiKey,
        string voiceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) throw Failure("There is no answer to speak.", "validation");
        if (string.IsNullOrWhiteSpace(apiKey)) throw Failure("Save an ElevenLabs API key first.", "credentials");
        if (string.IsNullOrWhiteSpace(voiceId)) throw Failure("Save an ElevenLabs voice ID first.", "voice");

        string body = JsonSerializer.Serialize(new { text = text.Trim(), model_id = "eleven_multilingual_v2" });
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"text-to-speech/{Uri.EscapeDataString(voiceId.Trim())}?output_format=mp3_44100_128");
        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey.Trim());
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

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
            throw Failure("ElevenLabs did not respond in time. Try again.", "timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw Failure("Could not reach ElevenLabs. Check the network and try again.", "send", inner: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw HttpFailure(response.StatusCode);
            byte[] bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0) throw Failure("ElevenLabs returned no audio. Try again.", "response");
            return new AudioPayload(bytes, "audio/mpeg");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumAudioBytes)
            throw Failure("ElevenLabs returned audio that is too large to play.", "response_limit");

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream output = new();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > MaximumAudioBytes)
                throw Failure("ElevenLabs returned audio that is too large to play.", "response_limit");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static SpeechServiceException HttpFailure(HttpStatusCode status)
    {
        int code = (int)status;
        string message = code switch
        {
            401 or 403 => "ElevenLabs rejected the API key. Save a valid key and try again.",
            404 or 422 => "ElevenLabs rejected the voice ID or voice settings. Check the saved voice ID.",
            429 => "ElevenLabs rate-limited the request. Wait and try again.",
            >= 500 => "ElevenLabs is temporarily unavailable. Try again later.",
            _ => "ElevenLabs rejected the speech request. Check the saved settings."
        };
        return Failure(message, "http", code);
    }

    private static SpeechServiceException Failure(
        string message,
        string stage,
        int? status = null,
        Exception? inner = null)
        => new(message, "elevenlabs", stage, status, inner);

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
