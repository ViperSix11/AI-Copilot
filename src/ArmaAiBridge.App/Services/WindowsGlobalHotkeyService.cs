using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int HotkeyId = 0x5042;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public WindowsGlobalHotkeyService(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        nint handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("The WPF window handle is unavailable.");
        _source.AddHook(WindowProc);
    }

    public event EventHandler? Activated;

    public GlobalHotkeyRegistrationResult Register(GlobalPushToTalkHotkey binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        binding.Validate();
        Unregister();
        uint modifiers = (uint)binding.Modifiers | ModNoRepeat;
        if (NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, modifiers, (uint)binding.VirtualKey))
        {
            _registered = true;
            return new GlobalHotkeyRegistrationResult(true, "registered", "Registered");
        }
        int error = Marshal.GetLastWin32Error();
        return error == 1409
            ? new GlobalHotkeyRegistrationResult(false, "conflict", "This global hotkey is already in use by another application.")
            : new GlobalHotkeyRegistrationResult(false, "registration_failed", $"Windows rejected the global hotkey (error {error}).");
    }

    public void Unregister()
    {
        if (!_registered) return;
        _ = NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam == (nint)HotkeyId)
        {
            handled = true;
            Activated?.Invoke(this, EventArgs.Empty);
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        _source.RemoveHook(WindowProc);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(nint hWnd, int id);
    }
}

public sealed class WindowsKeyStateService : IKeyStateService
{
    public bool IsKeyDown(int virtualKey) => NativeMethods.GetAsyncKeyState(virtualKey) < 0;

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);
    }
}
