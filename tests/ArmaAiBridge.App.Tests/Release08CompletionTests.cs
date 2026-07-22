using System.Text.Json;
using System.Diagnostics;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class Release08CompletionTests
{
    [Fact]
    public void VanillaSolver_IsDeterministicAndReportsSignedZeroCorrection()
    {
        StateBallisticProfile profile = Profile();
        VanillaBallisticSolver solver = new();
        BallisticSolutionRequest request = new(300, 90, 100, null);

        BallisticSolution first = solver.Solve(profile, request, 100, false);
        BallisticSolution second = solver.Solve(profile, request, 100, false);

        Assert.Equal(first, second);
        Assert.Equal("arma-vanilla-config", first.Model);
        Assert.Equal(90, first.BearingDegrees);
        Assert.Equal(300, first.RangeMeters);
        Assert.False(first.TerrainPointAssumed);
        Assert.False(first.WindCorrectionAvailable);
        Assert.InRange(first.TimeOfFlightSeconds, 0.3, 2.0);
        Assert.InRange(first.PredictedImpactVelocityMetersPerSecond, 100, 920);
        Assert.Equal(Math.Sign(first.ElevationCorrectionMilliradians) > 0 ? "high" : "low", first.HoldDirection);
    }

    [Fact]
    public void VanillaSolver_CoversModdedCartridgeGravityZeroingAndElevationFixturesWithinBoundedRuntime()
    {
        VanillaBallisticSolver solver = new();
        StateBallisticProfile rifle = Profile();
        Stopwatch runtime = Stopwatch.StartNew();
        BallisticSolution baseline = solver.Solve(rifle, new(600, 190, 100, null), 100, false);
        BallisticSolution lapua = solver.Solve(
            rifle with
            {
                WeaponClass = "srifle_LRR_F",
                WeaponDisplayName = ".338 LM rifle",
                AmmunitionClass = "B_338_Ball",
                AmmunitionDisplayName = ".338 LM",
                InitialSpeedMetersPerSecond = 910,
                TypicalSpeedMetersPerSecond = 910,
                AirFriction = -0.00055
            },
            new(600, 190, 100, null), 100, false);
        BallisticSolution reducedGravity = solver.Solve(
            rifle with { GravityCoefficient = 0.5 }, new(600, 190, 100, null), 100, false);
        BallisticSolution zeroedFarther = solver.Solve(
            rifle with { CurrentZeroingMeters = 500 }, new(600, 190, 100, null), 100, false);
        BallisticSolution above = solver.Solve(rifle, new(600, 190, 130, null), 130, false);
        BallisticSolution below = solver.Solve(rifle, new(600, 190, 70, null), 70, false);
        runtime.Stop();

        Assert.True(lapua.TimeOfFlightSeconds < baseline.TimeOfFlightSeconds);
        Assert.True(reducedGravity.RequiredElevationAngleDegrees < baseline.RequiredElevationAngleDegrees);
        Assert.True(zeroedFarther.ElevationCorrectionMilliradians < baseline.ElevationCorrectionMilliradians);
        Assert.True(above.RequiredElevationAngleDegrees > baseline.RequiredElevationAngleDegrees);
        Assert.True(below.RequiredElevationAngleDegrees < baseline.RequiredElevationAngleDegrees);
        Assert.True(runtime.Elapsed < TimeSpan.FromSeconds(2), $"Solver runtime: {runtime.Elapsed}");
    }

    [Fact]
    public void VanillaSolver_RejectsRocketAndMissingConfiguration()
    {
        VanillaBallisticSolver solver = new();
        Assert.Throws<InvalidOperationException>(() => solver.Solve(
            Profile() with { SupportedProjectileType = "", Simulation = "shotRocket" },
            new(300, 0, 100, null), 100, false));
        Assert.Throws<InvalidOperationException>(() => solver.Solve(
            Profile() with { Available = false, Reason = "missing_ballistic_config", InitialSpeedMetersPerSecond = 0 },
            new(300, 0, 100, null), 100, false));
    }

    [Fact]
    public void BallisticArguments_AreBoundedAndTargetPointUsesArmaBearing()
    {
        using JsonDocument valid = JsonDocument.Parse(
            """{"rangeMeters":300,"bearingDegrees":450,"targetElevationAslMeters":null,"targetHeightAboveTerrainMeters":2}""");
        BallisticSolutionRequest request = VanillaBallisticSolver.ParseRequest(valid.RootElement);
        Assert.Equal(90, request.BearingDegrees);
        (double x, double y) = VanillaBallisticSolver.TargetPoint(new WorldPosition(1000, 2000, 100), request.RangeMeters, request.BearingDegrees);
        Assert.Equal(1300, x, 8);
        Assert.Equal(2000, y, 8);

        using JsonDocument tooClose = JsonDocument.Parse(
            """{"rangeMeters":24,"bearingDegrees":0,"targetElevationAslMeters":null,"targetHeightAboveTerrainMeters":null}""");
        Assert.Throws<InvalidOperationException>(() => VanillaBallisticSolver.ParseRequest(tooClose.RootElement));
        using JsonDocument conflicting = JsonDocument.Parse(
            """{"rangeMeters":300,"bearingDegrees":0,"targetElevationAslMeters":10,"targetHeightAboveTerrainMeters":2}""");
        Assert.Throws<InvalidOperationException>(() => VanillaBallisticSolver.ParseRequest(conflicting.RootElement));
    }

    [Fact]
    public async Task BallisticTool_QueriesTerrainOnlyWhenElevationIsMissing()
    {
        int terrainCalls = 0;
        BallisticToolService tool = new((x, y, _) =>
        {
            terrainCalls++;
            Assert.Equal(1300, x, 6);
            Assert.Equal(2000, y, 6);
            return Task.FromResult(98d);
        });
        using JsonDocument arguments = JsonDocument.Parse(
            """{"rangeMeters":300,"bearingDegrees":90,"targetElevationAslMeters":null,"targetHeightAboveTerrainMeters":2}""");

        string json = await tool.CalculateAsync(arguments.RootElement, Profile(), TestContext.Current.CancellationToken);

        Assert.Equal(1, terrainCalls);
        using JsonDocument result = JsonDocument.Parse(json);
        JsonElement solution = result.RootElement.GetProperty("firingSolution");
        Assert.True(solution.GetProperty("terrainPointAssumed").GetBoolean());
        Assert.Equal(100, solution.GetProperty("targetElevationAslMeters").GetDouble());
        Assert.False(solution.GetProperty("windCorrectionAvailable").GetBoolean());
    }

    [Fact]
    public async Task BallisticTool_RejectsAdvancedBallisticsWithoutTerrainQuery()
    {
        int terrainCalls = 0;
        BallisticToolService tool = new((_, _, _) => { terrainCalls++; return Task.FromResult(0d); });
        using JsonDocument arguments = JsonDocument.Parse(
            """{"rangeMeters":300,"bearingDegrees":90,"targetElevationAslMeters":100,"targetHeightAboveTerrainMeters":null}""");

        string json = await tool.CalculateAsync(
            arguments.RootElement,
            Profile() with { Available = false, Reason = "advanced_ballistics_mod_detected", AdvancedBallisticsDetected = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, terrainCalls);
        Assert.Contains("advanced_ballistics_mod_detected", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BallisticToolSelection_RequiresAnExplicitFiringRequest()
    {
        Assert.True(AssistantRequestPolicy.RequiresBallisticTool("Firing solution, range 600, bearing 223."));
        Assert.True(AssistantRequestPolicy.RequiresBallisticTool("What holdover do I need?"));
        Assert.False(AssistantRequestPolicy.RequiresBallisticTool("What is my current weapon?"));
        Assert.False(AssistantRequestPolicy.RequiresBallisticTool("Is the target 600 metres away?"));
    }

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
    public async Task GlobalHotkey_RegistersDefaultIgnoresRepeatAndStopsOnceOnRelease()
    {
        FakeGlobalHotkey hotkey = new();
        FakeKeyState keys = new();
        TaskCompletionSource releasePoll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0, stops = 0;
        using GlobalPushToTalkController controller = new(
            hotkey,
            keys,
            (_, _) => { starts++; return Task.FromResult(true); },
            _ => { stops++; return Task.CompletedTask; },
            (_, token) => releasePoll.Task.WaitAsync(token));

        GlobalHotkeyRegistrationResult registration = controller.Configure(GlobalPushToTalkHotkey.Default);
        Assert.True(registration.Registered);
        Assert.Equal(GlobalHotkeyModifiers.Shift, hotkey.LastBinding!.Modifiers);
        Assert.Equal(0x20, hotkey.LastBinding.VirtualKey);

        hotkey.Raise();
        hotkey.Raise();
        Assert.Equal(1, starts);
        keys.Down = false;
        releasePoll.SetResult();
        await WaitUntilAsync(() => stops == 1);
        Assert.Equal(1, stops);
    }

    [Fact]
    public void GlobalHotkey_ReportsConflictWithoutFallbackAndDefersChangesWhileRecordingContract()
    {
        FakeGlobalHotkey hotkey = new() { Registration = new(false, "conflict", "in use") };
        using GlobalPushToTalkController controller = new(
            hotkey,
            new FakeKeyState(),
            (_, _) => Task.FromResult(false),
            _ => Task.CompletedTask);

        GlobalHotkeyRegistrationResult result = controller.Configure(GlobalPushToTalkHotkey.Default);
        Assert.False(result.Registered);
        Assert.Equal("conflict", result.Code);
        Assert.Equal(1, hotkey.RegisterCalls);
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
    public async Task GlobalHotkey_ChangingDuringRecordingKeepsFrozenReleaseKeyAndRegistersAfterRelease()
    {
        FakeGlobalHotkey hotkey = new();
        FakeKeyState keys = new();
        TaskCompletionSource releasePoll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int stops = 0;
        using GlobalPushToTalkController controller = new(
            hotkey,
            keys,
            (_, _) => Task.FromResult(true),
            _ => { stops++; return Task.CompletedTask; },
            (_, token) => releasePoll.Task.WaitAsync(token));
        controller.Configure(GlobalPushToTalkHotkey.Default);
        hotkey.Raise();
        GlobalPushToTalkHotkey next = new(true, GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift, 0x56);

        GlobalHotkeyRegistrationResult deferred = controller.Configure(next);

        Assert.Equal("deferred", deferred.Code);
        Assert.Equal(GlobalPushToTalkHotkey.Default, hotkey.LastBinding);
        keys.Down = false;
        releasePoll.SetResult();
        await WaitUntilAsync(() => stops == 1 && hotkey.LastBinding == next);
        Assert.Equal(0x20, keys.LastVirtualKey);
        Assert.Equal(next, hotkey.LastBinding);
    }

    [Fact]
    public void GlobalHotkey_DisableAndShutdownUnregisterWithoutFallback()
    {
        FakeGlobalHotkey hotkey = new();
        GlobalPushToTalkController controller = new(
            hotkey,
            new FakeKeyState(),
            (_, _) => Task.FromResult(false),
            _ => Task.CompletedTask);
        controller.Configure(GlobalPushToTalkHotkey.Default);
        int beforeDisable = hotkey.UnregisterCalls;

        GlobalHotkeyRegistrationResult disabled = controller.Configure(
            GlobalPushToTalkHotkey.Default with { Enabled = false });

        Assert.Equal("disabled", disabled.Code);
        Assert.True(hotkey.UnregisterCalls > beforeDisable);
        controller.Dispose();
        Assert.True(hotkey.Disposed);
        Assert.True(hotkey.UnregisterCalls > beforeDisable + 1);
    }

    [Fact]
    public void SqfAndSchemas_ExposeBoundedVanillaBallisticInputsWithoutArbitraryExecution()
    {
        string root = RepositoryRoot();
        string publish = File.ReadAllText(Path.Combine(root,
            "arma3/addon-source/arma_ai_bridge_client/functions/fn_publishStateSnapshot.sqf"));
        Assert.Contains("currentZeroing", publish, StringComparison.Ordinal);
        Assert.Contains("CfgMagazines", publish, StringComparison.Ordinal);
        Assert.Contains("CfgAmmo", publish, StringComparison.Ordinal);
        Assert.Contains("eyePos player", publish, StringComparison.Ordinal);
        Assert.Contains("ace_advanced_ballistics", publish, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allMissionObjects", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("cursorTarget", publish, StringComparison.OrdinalIgnoreCase);
        string execute = File.ReadAllText(Path.Combine(root,
            "arma3/addon-source/arma_ai_bridge_client/functions/fn_executeQuery.sqf"));
        Assert.Contains("query_terrain_height", execute, StringComparison.Ordinal);
        Assert.Contains("getTerrainHeightASL", execute, StringComparison.Ordinal);
        Assert.DoesNotContain("compile", execute, StringComparison.OrdinalIgnoreCase);
        string hotkey = File.ReadAllText(Path.Combine(root,
            "src/ArmaAiBridge.App/Services/WindowsGlobalHotkeyService.cs"));
        Assert.Contains("RegisterHotKey", hotkey, StringComparison.Ordinal);
        Assert.Contains("WmHotkey = 0x0312", hotkey, StringComparison.Ordinal);
        Assert.Contains("GetAsyncKeyState", hotkey, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowsHookEx", hotkey, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", hotkey, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenProcess", hotkey, StringComparison.Ordinal);
    }

    private static StateBallisticProfile Profile() => new(
        true, "", "arma-vanilla-config", "bullet",
        "arifle_MX_F", "MX 6.5 millimetre", "arifle_MX_F", "Single",
        "30Rnd_65x39_caseless_mag", "6.5 millimetre magazine",
        "B_65x39_Caseless", "6.5 millimetre bullet", "shotBullet",
        30, 200, 2, 800, -0.0008, 1, 800,
        new WorldPosition(1000, 2000, 100), false);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class FakeGlobalHotkey : IGlobalHotkeyService
    {
        public event EventHandler? Activated;
        public GlobalHotkeyRegistrationResult Registration { get; init; } = new(true, "registered", "Registered");
        public GlobalPushToTalkHotkey? LastBinding { get; private set; }
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }
        public bool Disposed { get; private set; }
        public GlobalHotkeyRegistrationResult Register(GlobalPushToTalkHotkey binding)
        {
            RegisterCalls++;
            LastBinding = binding;
            return Registration;
        }
        public void Raise() => Activated?.Invoke(this, EventArgs.Empty);
        public void Unregister() => UnregisterCalls++;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeKeyState : IKeyStateService
    {
        public bool Down { get; set; } = true;
        public int LastVirtualKey { get; private set; }
        public bool IsKeyDown(int virtualKey) { LastVirtualKey = virtualKey; return Down; }
    }
}
