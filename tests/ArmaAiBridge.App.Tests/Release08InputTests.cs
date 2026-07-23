using System.Text.Json;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class Release08InputTests
{
    [Fact]
    public void SpeechNormalizer_ExpandsUnitsNumbersAndOnlyFormatsTheCurrentCallsignForSpeech()
    {
        string visible = "Alpha 1-1, target -2.5 m low at 1.2 km, 3 mils, 18°C ASL, wind 4 m/s, .338 LM FCS.";
        string spoken = RadioSpeechTextNormalizer.Normalize(visible, "Alpha 1-1");

        Assert.Equal(
            "Alpha One-One, target minus two point five metres low at one point two kilometres, three milliradians, eighteen degrees Celsius above sea level, wind four metres per second, point three three eight Lapua Magnum fire-control system.",
            spoken);
        Assert.Equal("Alpha One-One", CallsignSpeechFormatter.FormatCallsign("Alpha 1-1"));
        Assert.DoesNotContain("Alpha 1-1", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("m/s", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ASL", spoken, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FCS", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GlobalRawInput_StartsOnPressAndStopsOnceOnRelease()
    {
        FakeGlobalInput input = new();
        int starts = 0, stops = 0;
        using GlobalPushToTalkController controller = new(
            input,
            (_, _) => { starts++; return Task.FromResult(true); },
            _ => { stops++; return Task.CompletedTask; });

        GlobalInputRegistrationResult registration = controller.Configure(GlobalPushToTalkHotkey.Default);
        Assert.True(registration.Registered);
        Assert.Equal(GlobalHotkeyModifiers.Shift, input.Binding.Modifiers);
        Assert.Equal(0x20, input.Binding.VirtualKey);

        input.Press();
        input.Press();
        Assert.Equal(1, starts);
        input.Release();
        input.Release();
        await WaitUntilAsync(() => stops == 1);
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task EnablingGlobalPushToTalk_ExitsDetectionOnlyModeAndRecords()
    {
        FakeGlobalInput input = new();
        int starts = 0, stops = 0;
        using GlobalPushToTalkController controller = new(
            input,
            (_, _) => { starts++; return Task.FromResult(true); },
            _ => { stops++; return Task.CompletedTask; })
        {
            DetectionOnly = true
        };

        controller.Configure(GlobalPushToTalkHotkey.Default);

        Assert.False(controller.DetectionOnly);
        input.Press();
        input.Release();
        await WaitUntilAsync(() => stops == 1);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void GlobalRawInput_ReportsRegistrationFailureWithoutFallback()
    {
        FakeGlobalInput input = new() { Registration = new(false, "raw-input-registration-failed", "unavailable") };
        using GlobalPushToTalkController controller = new(
            input,
            (_, _) => Task.FromResult(false),
            _ => Task.CompletedTask);

        GlobalInputRegistrationResult result = controller.Configure(GlobalPushToTalkHotkey.Default);
        Assert.False(result.Registered);
        Assert.Equal("raw-input-registration-failed", result.Code);
        Assert.Equal(GlobalPushToTalkHotkey.Default, controller.Binding);
    }

    [Fact]
    public void GlobalHotkey_DefaultAndCustomBindingRoundTripThroughExistingSettings()
    {
        Assert.True(GlobalPushToTalkHotkey.Default.Enabled);
        Assert.Equal(GlobalHotkeyModifiers.Shift, GlobalPushToTalkHotkey.Default.Modifiers);
        Assert.Equal(0x20, GlobalPushToTalkHotkey.Default.VirtualKey);
        AppSettings original = new()
        {
            GlobalPushToTalkHotkey = new(true, GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift, 0x56)
        };

        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original))!;

        Assert.Equal(original.GlobalPushToTalkHotkey, restored.GlobalPushToTalkHotkey);
        Assert.Equal("Control + Shift + V", restored.GlobalPushToTalkHotkey.DisplayName);
    }

    [Fact]
    public void PushToTalkRecordingPolicy_CancelsSubMinimumTapBeforeSubmission()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(200), PushToTalkRecordingPolicy.MinimumUsefulDuration);
        Assert.False(PushToTalkRecordingPolicy.ShouldSubmit(TimeSpan.FromMilliseconds(199)));
        Assert.True(PushToTalkRecordingPolicy.ShouldSubmit(TimeSpan.FromMilliseconds(200)));
        Assert.True(PushToTalkRecordingPolicy.ShouldSubmit(WindowsMicrophoneCaptureService.MaximumDuration));
    }

    [Fact]
    public async Task GlobalRawInput_ChangingDuringRecordingAppliesAfterRelease()
    {
        FakeGlobalInput input = new();
        int stops = 0;
        using GlobalPushToTalkController controller = new(
            input,
            (_, _) => Task.FromResult(true),
            _ => { stops++; return Task.CompletedTask; });
        controller.Configure(GlobalPushToTalkHotkey.Default);
        input.Press();
        GlobalPushToTalkHotkey next = new(true, GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift, 0x56);

        GlobalInputRegistrationResult deferred = controller.Configure(next);

        Assert.Equal("deferred", deferred.Code);
        Assert.Equal(GlobalPushToTalkHotkey.Default, input.Binding);
        input.Release();
        await WaitUntilAsync(() => stops == 1 && input.Binding == next);
        Assert.Equal(next, input.Binding);
    }

    [Fact]
    public void GlobalRawInput_DisableKeepsProcessRegistrationAndControllerDoesNotOwnService()
    {
        FakeGlobalInput input = new();
        GlobalPushToTalkController controller = new(
            input,
            (_, _) => Task.FromResult(false),
            _ => Task.CompletedTask);
        controller.Configure(GlobalPushToTalkHotkey.Default);

        GlobalInputRegistrationResult disabled = controller.Configure(
            GlobalPushToTalkHotkey.Default with { Enabled = false });

        Assert.Equal("disabled", disabled.Code);
        controller.Dispose();
        Assert.False(input.Disposed);
    }

    [Fact]
    public void RuntimeHasNoFiringCalculationOrAceIntegration()
    {
        string root = RepositoryRoot();
        foreach (string relative in RuntimeFiles(root))
        {
            string source = File.ReadAllText(relative);
            Assert.DoesNotContain("calculate_firing_solution", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("calculate_ace", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ace_advanced_ballistics", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BallisticProfile", source, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> RuntimeFiles(string root)
    {
        foreach (string directory in new[] { "src", "arma3", "schemas" })
        {
            string full = Path.Combine(root, directory);
            foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return file;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(
                   directory.FullName, "src", "ArmaAiBridge.App", "ArmaAiBridge.App.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class FakeGlobalInput : IGlobalPushToTalkInputService
    {
        public event EventHandler? HotkeyPressed;
        public event EventHandler? HotkeyReleased;
        public event EventHandler<GlobalInputRegistrationResult>? RegistrationStatusChanged;
        public GlobalInputRegistrationResult Registration { get; init; } = new(true, "raw-input-registered", "Registered");
        public GlobalPushToTalkHotkey Binding { get; private set; } = GlobalPushToTalkHotkey.Default;
        public bool Disposed { get; private set; }
        public GlobalInputRegistrationResult Configure(GlobalPushToTalkHotkey binding)
        {
            Binding = binding;
            return binding.Enabled ? Registration : GlobalInputRegistrationResult.Disabled;
        }
        public void Press() => HotkeyPressed?.Invoke(this, EventArgs.Empty);
        public void Release() => HotkeyReleased?.Invoke(this, EventArgs.Empty);
        public void SuspendRecognition() { }
        public GlobalInputRegistrationResult ResumeRecognition() => Registration;
        public void Status(GlobalInputRegistrationResult result) => RegistrationStatusChanged?.Invoke(this, result);
        public void Dispose() => Disposed = true;
    }
}
