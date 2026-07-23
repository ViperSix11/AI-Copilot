using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class BackgroundRawInputTests
{
    private const ushort Break = 0x0001;
    private const ushort E0 = 0x0002;

    [Fact]
    public void Registration_UsesExactlyGenericKeyboardInputSinkAndStableTarget()
    {
        FakeNative native = new();
        using WindowsRawInputHotkeyService service = new((nint)1234, native);
        Assert.True(service.Registration.Registered);
        Assert.Equal("raw-input-registered", service.Registration.Code);
        Assert.Equal(new RawInputDeviceRegistration(0x01, 0x06, 0x100, (nint)1234), native.Request);
        Assert.Equal(native.Request, native.Verified);
        Assert.Equal(1, native.RegisterCalls);
    }

    [Theory]
    [InlineData(false, true, 5, "raw-input-registration-failed")]
    [InlineData(false, true, 1167, "raw-input-device-unavailable")]
    [InlineData(true, false, 0, "raw-input-verification-failed")]
    public void Registration_FailsClosedWithStableStatusCodes(bool register, bool verify, int error, string code)
    {
        FakeNative native = new() { RegisterResult = register, VerifyResult = verify, Error = error };
        using WindowsRawInputHotkeyService service = new((nint)22, native);
        Assert.False(service.Registration.Registered);
        Assert.Equal(code, service.Registration.Code);
    }

    [Fact]
    public void Matcher_AcceptsLeftOrRightShift_IgnoresRepeats_AndReleasesWhenModifierWasReleasedFirst()
    {
        RawKeyboardChordMatcher matcher = new();
        matcher.Configure(GlobalPushToTalkHotkey.Default);
        Assert.Equal(RawChordTransition.None, matcher.Process(Key(1, 0x10, makeCode: 0x2A)));
        Assert.Equal(RawChordTransition.Pressed, matcher.Process(Key(1, 0x20)));
        Assert.Equal(RawChordTransition.None, matcher.Process(Key(1, 0x20)));
        Assert.Equal(RawChordTransition.None, matcher.Process(Key(1, 0x10, Break, 0x2A)));
        Assert.Equal(RawChordTransition.Released, matcher.Process(Key(1, 0x20, Break)));
        Assert.Equal(RawChordTransition.None, matcher.Process(Key(2, 0x10, makeCode: 0x36)));
        Assert.Equal(RawChordTransition.Pressed, matcher.Process(Key(2, 0x20)));
        Assert.Equal(RawChordTransition.Released, matcher.Process(Key(2, 0x20, Break)));
    }

    [Fact]
    public void Matcher_NormalizesLeftAndRightControlAltAndWindows()
    {
        Assert.Equal(0xA2, RawKeyboardChordMatcher.NormalizeVirtualKey(0x11, 0, 0));
        Assert.Equal(0xA3, RawKeyboardChordMatcher.NormalizeVirtualKey(0x11, 0, E0));
        Assert.Equal(0xA4, RawKeyboardChordMatcher.NormalizeVirtualKey(0x12, 0, 0));
        Assert.Equal(0xA5, RawKeyboardChordMatcher.NormalizeVirtualKey(0x12, 0, E0));
        Assert.Equal(0x5B, RawKeyboardChordMatcher.NormalizeVirtualKey(0x5B, 0, E0));
        Assert.Equal(0x5C, RawKeyboardChordMatcher.NormalizeVirtualKey(0x5C, 0, E0));
    }

    [Fact]
    public void Matcher_AggregatesModifiersAcrossKeyboardsButOnlyActivePrimaryReleaseStops()
    {
        RawKeyboardChordMatcher matcher = new();
        matcher.Configure(new(true, GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift, 0x56));
        matcher.Process(Key(11, 0x11));
        matcher.Process(Key(12, 0x10, makeCode: 0x36));
        Assert.Equal(RawChordTransition.Pressed, matcher.Process(Key(13, 0x56)));
        Assert.Equal(RawChordTransition.None, matcher.Process(Key(14, 0x56, Break)));
        Assert.Equal(RawChordTransition.Released, matcher.Process(Key(13, 0x56, Break)));
    }

    [Fact]
    public void Matcher_RequiresExactModifiersAndDisabledOrSuspendedNeverActivates()
    {
        RawKeyboardChordMatcher matcher = new();
        matcher.Configure(GlobalPushToTalkHotkey.Default);
        matcher.Process(Key(1, 0x10, makeCode: 0x2A));
        matcher.Process(Key(1, 0x11));
        Assert.Equal(RawChordTransition.None, matcher.Process(Key(1, 0x20)));
        matcher.Process(Key(1, 0x20, Break));
        matcher.Process(Key(1, 0x11, Break));
        matcher.Suspended = true;
        Assert.Equal(RawChordTransition.None, matcher.Process(Key(1, 0x20)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForegroundAndBackgroundPacketsActivateAndReleaseExactlyOnce(bool background)
    {
        FakeNative native = new();
        using WindowsRawInputHotkeyService service = new((nint)88, native);
        service.Configure(GlobalPushToTalkHotkey.Default);
        int starts = 0, stops = 0;
        service.HotkeyPressed += (_, _) => starts++;
        service.HotkeyReleased += (_, _) => stops++;
        Assert.False(service.ProcessPacket(Key(7, 0x10, makeCode: 0x2A), background));
        Assert.True(service.ProcessPacket(Key(7, 0x20), background));
        Assert.True(service.ProcessPacket(Key(7, 0x20, Break), background));
        Assert.Equal(1, starts); Assert.Equal(1, stops);
    }

    [Fact]
    public void BackgroundRegression_DoesNotRequireAnyRegisterHotKeyMessage()
    {
        FakeNative native = new();
        using WindowsRawInputHotkeyService service = new((nint)99, native);
        service.Configure(GlobalPushToTalkHotkey.Default);
        int starts = 0, stops = 0;
        service.HotkeyPressed += (_, _) => starts++;
        service.HotkeyReleased += (_, _) => stops++;
        service.ProcessPacket(Key(9, 0x10, makeCode: 0x2A), background: true);
        service.ProcessPacket(Key(9, 0x20), background: true);
        service.ProcessPacket(Key(9, 0x20, Break), background: true);
        Assert.Equal(1, starts); Assert.Equal(1, stops);
    }

    [Fact]
    public void MalformedFlagsAndVirtualKeysAreRejected()
    {
        using WindowsRawInputHotkeyService service = new((nint)101, new FakeNative());
        service.Configure(GlobalPushToTalkHotkey.Default);
        Assert.False(service.ProcessPacket(Key(1, 0x20, 0x0080), true));
        Assert.False(service.ProcessPacket(Key(1, 0x07), true));
        Assert.False(service.ProcessPacket(Key(1, 0xFF), true));
    }

    [Fact]
    public async Task Controller_StopsAtFifteenSecondSafetyDeadlineWithoutPolling()
    {
        FakeInput input = new();
        TaskCompletionSource deadline = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan requested = TimeSpan.Zero;
        int stops = 0;
        using GlobalPushToTalkController controller = new(
            input,
            (_, _) => Task.FromResult(true),
            _ => { stops++; return Task.CompletedTask; },
            (duration, _) => { requested = duration; return deadline.Task; });
        controller.Configure(GlobalPushToTalkHotkey.Default);
        input.Press();
        Assert.Equal(TimeSpan.FromSeconds(15), requested);
        deadline.SetResult();
        await WaitUntilAsync(() => stops == 1);
        input.Release();
        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task Controller_ShutdownDuringRecordingStopsOnceAndDetaches()
    {
        FakeInput input = new();
        TaskCompletionSource never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int stops = 0;
        GlobalPushToTalkController controller = new(input, (_, _) => Task.FromResult(true),
            _ => { stops++; return Task.CompletedTask; }, (_, token) => never.Task.WaitAsync(token));
        controller.Configure(GlobalPushToTalkHotkey.Default);
        input.Press();
        controller.Dispose();
        await WaitUntilAsync(() => stops == 1);
        input.Press(); input.Release();
        Assert.Equal(1, stops);
    }

    [Fact]
    public void StaticContract_HasNoHooksInjectionPollingOrKeyContentLogging()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(),
            "src/ArmaAiBridge.App/Services/WindowsRawInputHotkeyService.cs"));
        Assert.DoesNotContain("SetWindowsHookEx", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenProcess", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAsyncKeyState", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RIDEV_NOLEGACY", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RIDEV_NOHOTKEYS", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RIDEV_EXINPUTSINK", source, StringComparison.OrdinalIgnoreCase);
    }

    private static RawKeyboardPacket Key(long device, ushort key, ushort flags = 0, ushort makeCode = 0)
        => new((nint)device, makeCode, flags, key, 0, 0);

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
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class FakeNative : IRawInputNativeApi
    {
        public bool RegisterResult { get; init; } = true;
        public bool VerifyResult { get; init; } = true;
        public int Error { get; init; }
        public int RegisterCalls { get; private set; }
        public RawInputDeviceRegistration Request { get; private set; }
        public RawInputDeviceRegistration Verified { get; private set; }
        public bool Register(RawInputDeviceRegistration registration, out int error) { RegisterCalls++; Request = registration; error = Error; return RegisterResult; }
        public bool Verify(RawInputDeviceRegistration expected, out int error) { Verified = expected; error = Error; return VerifyResult; }
        public bool Unregister(out int error) { error = 0; return true; }
        public bool TryReadKeyboard(nint rawInputHandle, out RawKeyboardPacket packet, out int error) { packet = default; error = 0; return false; }
    }

    private sealed class FakeInput : IGlobalPushToTalkInputService
    {
        public event EventHandler? HotkeyPressed;
        public event EventHandler? HotkeyReleased;
        public event EventHandler<GlobalInputRegistrationResult>? RegistrationStatusChanged { add { } remove { } }
        public GlobalInputRegistrationResult Registration { get; } = new(true, "raw-input-registered", "Registered");
        public GlobalPushToTalkHotkey Binding { get; private set; } = GlobalPushToTalkHotkey.Default;
        public GlobalInputRegistrationResult Configure(GlobalPushToTalkHotkey binding) { Binding = binding; return Registration; }
        public void SuspendRecognition() { }
        public GlobalInputRegistrationResult ResumeRecognition() => Registration;
        public void Press() => HotkeyPressed?.Invoke(this, EventArgs.Empty);
        public void Release() => HotkeyReleased?.Invoke(this, EventArgs.Empty);
        public void Dispose() { }
    }
}
