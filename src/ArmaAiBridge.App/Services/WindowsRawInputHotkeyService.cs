using System.Runtime.InteropServices;
using System.Windows.Interop;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public readonly record struct RawKeyboardPacket(nint DeviceHandle, ushort MakeCode, ushort Flags,
    ushort VirtualKey, uint Message, uint ExtraInformation);
public readonly record struct RawInputDeviceRegistration(ushort UsagePage, ushort Usage, uint Flags, nint TargetWindow);

public interface IRawInputNativeApi
{
    bool Register(RawInputDeviceRegistration registration, out int windowsError);
    bool Verify(RawInputDeviceRegistration expected, out int windowsError);
    bool Unregister(out int windowsError);
    bool TryReadKeyboard(nint rawInputHandle, out RawKeyboardPacket packet, out int windowsError);
}

public sealed record GlobalInputRegistrationResult(bool Registered, string Code, string Message,
    int WindowsError = 0, string Mode = "raw-input-background")
{
    public static GlobalInputRegistrationResult Disabled { get; } =
        new(true, "disabled", "Background Raw Input registered; push-to-talk disabled.");
    public static GlobalInputRegistrationResult Deferred { get; } =
        new(true, "deferred", "Binding change deferred until recording ends.");
}

public interface IGlobalPushToTalkInputService : IDisposable
{
    event EventHandler? HotkeyPressed;
    event EventHandler? HotkeyReleased;
    event EventHandler<GlobalInputRegistrationResult>? RegistrationStatusChanged;
    GlobalInputRegistrationResult Registration { get; }
    GlobalPushToTalkHotkey Binding { get; }
    GlobalInputRegistrationResult Configure(GlobalPushToTalkHotkey binding);
    void SuspendRecognition();
    GlobalInputRegistrationResult ResumeRecognition();
}

public sealed class WindowsRawInputHotkeyService : IGlobalPushToTalkInputService
{
    public const int WmInput = 0x00FF;
    public const uint RimInput = 0;
    public const uint RimInputSink = 1;
    public const uint RidevInputSink = 0x00000100;
    public const uint RidevExInputSink = 0x00001000;
    public const uint RidevNoLegacy = 0x00000030;
    public const uint RidevNoHotKeys = 0x00000200;
    public const ushort GenericDesktopUsagePage = 0x01;
    public const ushort KeyboardUsage = 0x06;

    private readonly IRawInputNativeApi _native;
    private readonly HwndSource? _source;
    private readonly RawKeyboardChordMatcher _matcher = new();
    private readonly RawInputDeviceRegistration _request;
    private bool _disposed;

    public WindowsRawInputHotkeyService()
    {
        HwndSourceParameters parameters = new("PapaBearBackgroundRawInput")
        {
            Width = 0, Height = 0, PositionX = -32000, PositionY = -32000, WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WindowProc);
        _native = new WindowsRawInputNativeApi();
        _request = KeyboardRegistration(_source.Handle);
        Registration = RegisterAndVerify();
    }

    public WindowsRawInputHotkeyService(nint stableTargetWindow, IRawInputNativeApi native)
    {
        if (stableTargetWindow == 0) throw new ArgumentOutOfRangeException(nameof(stableTargetWindow));
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _request = KeyboardRegistration(stableTargetWindow);
        Registration = RegisterAndVerify();
    }

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;
    public event EventHandler<GlobalInputRegistrationResult>? RegistrationStatusChanged;
    public GlobalInputRegistrationResult Registration { get; private set; }
    public GlobalPushToTalkHotkey Binding => _matcher.Binding;

    public GlobalInputRegistrationResult Configure(GlobalPushToTalkHotkey binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        binding.Validate();
        _matcher.Configure(binding);
        return binding.Enabled ? Registration : GlobalInputRegistrationResult.Disabled;
    }

    public void SuspendRecognition() => _matcher.Suspended = true;
    public GlobalInputRegistrationResult ResumeRecognition()
    {
        _matcher.Suspended = false;
        return _matcher.Binding.Enabled ? Registration : GlobalInputRegistrationResult.Disabled;
    }

    public bool ProcessPacket(RawKeyboardPacket packet, bool background)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Registration.Registered || (packet.Flags & ~0x0007) != 0 || packet.VirtualKey is < 0x08 or > 0xFE)
            return false;
        RawChordTransition transition = _matcher.Process(packet);
        if (transition == RawChordTransition.Pressed) HotkeyPressed?.Invoke(this, EventArgs.Empty);
        else if (transition == RawChordTransition.Released) HotkeyReleased?.Invoke(this, EventArgs.Empty);
        return transition != RawChordTransition.None;
    }

    private GlobalInputRegistrationResult RegisterAndVerify()
    {
        if (!_native.Register(_request, out int error))
            return Publish(new(false, error == 1167 ? "raw-input-device-unavailable" : "raw-input-registration-failed",
                $"Raw Input registration failed - Windows error {error}.", error));
        if (!_native.Verify(_request, out error))
            return Publish(new(false, "raw-input-verification-failed",
                error == 0 ? "Raw Input registration verification failed." : $"Raw Input registration verification failed - Windows error {error}.", error));
        return Publish(new(true, "raw-input-registered", "Keyboard input registered."));
    }

    private GlobalInputRegistrationResult Publish(GlobalInputRegistrationResult value)
    {
        Registration = value;
        RegistrationStatusChanged?.Invoke(this, value);
        return value;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmInput) return 0;
        uint source = unchecked((uint)wParam.ToInt64());
        if (source is RimInput or RimInputSink && _native.TryReadKeyboard(lParam, out RawKeyboardPacket packet, out _))
            _ = ProcessPacket(packet, source == RimInputSink);
        handled = false;
        return 0;
    }

    private static RawInputDeviceRegistration KeyboardRegistration(nint target)
        => new(GenericDesktopUsagePage, KeyboardUsage, RidevInputSink, target);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _matcher.Suspended = true;
        _ = _native.Unregister(out _);
        if (_source is not null) { _source.RemoveHook(WindowProc); _source.Dispose(); }
    }
}

public enum RawChordTransition { None, Pressed, Released }

public sealed class RawKeyboardChordMatcher
{
    private readonly HashSet<(nint Device, int Key)> _down = new();
    private (nint Device, int Key)? _activePrimary;
    public GlobalPushToTalkHotkey Binding { get; private set; } = GlobalPushToTalkHotkey.Default;
    public bool Suspended { get; set; }
    public void Configure(GlobalPushToTalkHotkey binding) { binding.Validate(); Binding = binding; }

    public RawChordTransition Process(RawKeyboardPacket packet)
    {
        int key = NormalizeVirtualKey(packet.VirtualKey, packet.MakeCode, packet.Flags);
        bool isBreak = (packet.Flags & 0x0001) != 0;
        (nint Device, int Key) physical = (packet.DeviceHandle, key);
        if (isBreak)
        {
            bool wasDown = _down.Remove(physical);
            if (wasDown && _activePrimary == physical) { _activePrimary = null; return RawChordTransition.Released; }
            return RawChordTransition.None;
        }
        if (!_down.Add(physical)) return RawChordTransition.None;
        if (Suspended || !Binding.Enabled || key != Binding.VirtualKey || _activePrimary is not null ||
            !RequiredModifiersDown(Binding.Modifiers)) return RawChordTransition.None;
        _activePrimary = physical;
        return RawChordTransition.Pressed;
    }

    private bool RequiredModifiersDown(GlobalHotkeyModifiers required)
    {
        GlobalHotkeyModifiers actual = GlobalHotkeyModifiers.None;
        foreach ((_, int key) in _down)
        {
            if (key is 0xA0 or 0xA1) actual |= GlobalHotkeyModifiers.Shift;
            else if (key is 0xA2 or 0xA3) actual |= GlobalHotkeyModifiers.Control;
            else if (key is 0xA4 or 0xA5) actual |= GlobalHotkeyModifiers.Alt;
            else if (key is 0x5B or 0x5C) actual |= GlobalHotkeyModifiers.Windows;
        }
        return (actual & required) == required && (actual & ~required) == 0;
    }

    public static int NormalizeVirtualKey(ushort virtualKey, ushort makeCode, ushort flags)
        => virtualKey switch
        {
            0x10 => makeCode == 0x36 ? 0xA1 : 0xA0,
            0x11 => (flags & 0x0002) != 0 ? 0xA3 : 0xA2,
            0x12 => (flags & 0x0002) != 0 ? 0xA5 : 0xA4,
            _ => virtualKey
        };
}

internal sealed class WindowsRawInputNativeApi : IRawInputNativeApi
{
    private const uint RidInput = 0x10000003, RimTypeKeyboard = 1, RidevRemove = 1;
    private const int MaximumRawInputBytes = 1024;
    public bool Register(RawInputDeviceRegistration value, out int error)
    {
        NativeRawInputDevice[] devices = { ToNative(value) };
        bool ok = Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeRawInputDevice>());
        error = ok ? 0 : Marshal.GetLastWin32Error(); return ok;
    }
    public bool Verify(RawInputDeviceRegistration expected, out int error)
    {
        uint count = 0, size = (uint)Marshal.SizeOf<NativeRawInputDevice>();
        uint first = Native.GetRegisteredRawInputDevices(null, ref count, size);
        if (first == uint.MaxValue || count is 0 or > 64) { error = Marshal.GetLastWin32Error(); return false; }
        NativeRawInputDevice[] devices = new NativeRawInputDevice[count];
        uint returned = Native.GetRegisteredRawInputDevices(devices, ref count, size);
        if (returned == uint.MaxValue) { error = Marshal.GetLastWin32Error(); return false; }
        error = 0;
        return devices.Take((int)returned).Any(item => item.UsagePage == expected.UsagePage && item.Usage == expected.Usage &&
            item.TargetWindow == expected.TargetWindow && item.Flags == WindowsRawInputHotkeyService.RidevInputSink);
    }
    public bool Unregister(out int error)
    {
        NativeRawInputDevice[] devices = { new() { UsagePage = 1, Usage = 6, Flags = RidevRemove, TargetWindow = 0 } };
        bool ok = Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeRawInputDevice>());
        error = ok ? 0 : Marshal.GetLastWin32Error(); return ok;
    }
    public bool TryReadKeyboard(nint handle, out RawKeyboardPacket packet, out int error)
    {
        packet = default; uint bytes = 0, headerSize = (uint)Marshal.SizeOf<NativeRawInputHeader>();
        uint query = Native.GetRawInputData(handle, RidInput, 0, ref bytes, headerSize);
        int minimum = Marshal.SizeOf<NativeRawInputHeader>() + Marshal.SizeOf<NativeRawKeyboard>();
        if (query == uint.MaxValue || bytes < minimum || bytes > MaximumRawInputBytes) { error = Marshal.GetLastWin32Error(); return false; }
        nint buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            uint requested = bytes, read = Native.GetRawInputData(handle, RidInput, buffer, ref requested, headerSize);
            if (read == uint.MaxValue || read != bytes || requested != bytes) { error = Marshal.GetLastWin32Error(); return false; }
            NativeRawInputHeader header = Marshal.PtrToStructure<NativeRawInputHeader>(buffer);
            if (header.Type != RimTypeKeyboard || header.Size != bytes) { error = 0; return false; }
            NativeRawKeyboard keyboard = Marshal.PtrToStructure<NativeRawKeyboard>(buffer + Marshal.SizeOf<NativeRawInputHeader>());
            packet = new(header.Device, keyboard.MakeCode, keyboard.Flags, keyboard.VirtualKey, keyboard.Message, keyboard.ExtraInformation);
            error = 0; return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static NativeRawInputDevice ToNative(RawInputDeviceRegistration v) => new() { UsagePage = v.UsagePage, Usage = v.Usage, Flags = v.Flags, TargetWindow = v.TargetWindow };
    [StructLayout(LayoutKind.Sequential)] private struct NativeRawInputDevice { public ushort UsagePage, Usage; public uint Flags; public nint TargetWindow; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRawInputHeader { public uint Type, Size; public nint Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRawKeyboard { public ushort MakeCode, Flags, Reserved, VirtualKey; public uint Message, ExtraInformation; }
    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterRawInputDevices([In] NativeRawInputDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRegisteredRawInputDevices([Out] NativeRawInputDevice[]? devices, ref uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint GetRawInputData(nint rawInput, uint command, nint data, ref uint size, uint headerSize);
    }
}
