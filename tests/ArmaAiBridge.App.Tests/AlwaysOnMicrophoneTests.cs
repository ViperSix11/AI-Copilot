using System.Buffers.Binary;
using System.Text.Json;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class AlwaysOnMicrophoneTests
{
    private const int FrameMilliseconds = 100;

    [Fact]
    public void SilenceNeverCreatesAnUtterance()
    {
        PcmVoiceActivitySegmenter segmenter = new();
        byte[] silence = Frame(0);

        for (int index = 0; index < 200; index++)
            Assert.Null(segmenter.Process(silence));

        Assert.False(segmenter.IsCapturingUtterance);
    }

    [Fact]
    public void SustainedSpeechAndTrailingSilenceCreateExactlyOneBoundedUtterance()
    {
        PcmVoiceActivitySegmenter segmenter = new();
        byte[] silence = Frame(0);
        byte[] speech = Frame(6000);
        List<byte[]> completed = new();

        for (int index = 0; index < 3; index++)
            Assert.Null(segmenter.Process(silence));
        for (int index = 0; index < 5; index++)
        {
            byte[]? utterance = segmenter.Process(speech);
            if (utterance is not null) completed.Add(utterance);
        }
        for (int index = 0; index < 12; index++)
        {
            byte[]? utterance = segmenter.Process(silence);
            if (utterance is not null) completed.Add(utterance);
        }

        byte[] result = Assert.Single(completed);
        Assert.InRange(
            result.Length,
            BytesFor(TimeSpan.FromSeconds(1)),
            BytesFor(VoiceActivatedCapturePolicy.MaximumUtteranceDuration));
        Assert.False(segmenter.IsCapturingUtterance);
    }

    [Fact]
    public void ShortNoiseBurstIsDiscardedLocally()
    {
        PcmVoiceActivitySegmenter segmenter = new();
        byte[] silence = Frame(0);
        byte[] noise = Frame(12000);

        Assert.Null(segmenter.Process(noise));
        Assert.Null(segmenter.Process(noise));
        for (int index = 0; index < 12; index++)
            Assert.Null(segmenter.Process(silence));

        Assert.False(segmenter.IsCapturingUtterance);
    }

    [Fact]
    public void ContinuousSpeechStopsAtTheFifteenSecondLimit()
    {
        PcmVoiceActivitySegmenter segmenter = new();
        byte[] speech = Frame(6000);
        byte[]? completed = null;

        for (int index = 0; index < 200 && completed is null; index++)
            completed = segmenter.Process(speech);

        Assert.NotNull(completed);
        Assert.Equal(BytesFor(VoiceActivatedCapturePolicy.MaximumUtteranceDuration), completed.Length);
        Assert.False(segmenter.IsCapturingUtterance);
    }

    [Fact]
    public void AlwaysOnSettingIsOptInAndRoundTrips()
    {
        Assert.False(new AppSettings().MicrophoneAlwaysOn);
        AppSettings original = new() { MicrophoneAlwaysOn = true };

        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(original))!;

        Assert.True(restored.MicrophoneAlwaysOn);
    }

    [Fact]
    public void AssistantUiUsesTheSharedVoiceTurnAndDocumentsThePrivacyBoundary()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        string panel = File.ReadAllText(Path.Combine(root, "src", "ArmaAiBridge.App", "AssistantPanel.cs"));

        Assert.Contains("Mic always on (voice activated)", panel, StringComparison.Ordinal);
        Assert.Contains("with no wake word", panel, StringComparison.Ordinal);
        Assert.Contains("Voice activity is detected locally", panel, StringComparison.Ordinal);
        Assert.Contains("RunAssistantRecordingAsync(recording, progress, request", panel, StringComparison.Ordinal);
        Assert.Contains("VoiceStage.Listening", panel, StringComparison.Ordinal);
        Assert.Contains("settings.MicrophoneAlwaysOn", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("transcript => _log", panel, StringComparison.Ordinal);
    }

    private static int BytesFor(TimeSpan duration)
        => checked((int)(
            WindowsMicrophoneCaptureService.SampleRate *
            (WindowsMicrophoneCaptureService.BitsPerSample / 8d) *
            WindowsMicrophoneCaptureService.Channels *
            duration.TotalSeconds));

    private static byte[] Frame(short amplitude)
    {
        int sampleCount = WindowsMicrophoneCaptureService.SampleRate * FrameMilliseconds / 1000;
        byte[] frame = new byte[sampleCount * sizeof(short)];
        for (int offset = 0; offset < frame.Length; offset += sizeof(short))
            BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(offset, sizeof(short)), amplitude);
        return frame;
    }
}
