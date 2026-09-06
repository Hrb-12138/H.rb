using System.ComponentModel;
using System.Runtime.InteropServices;
using CnInstantTranslator.Native;

namespace CnInstantTranslator.Core;

public sealed class LowLevelMouseHook : IDisposable
{
    private IntPtr _hookHandle;
    private NativeMethods.LowLevelMouseProc? _proc;

    public event Action? LeftButtonDown;
    public event Action? LeftButtonUp;

    public bool IsLeftButtonDown { get; private set; }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _proc = HookCallback;
        _hookHandle = NativeMethods.SetWindowsHookExW(
            NativeMethods.WhMouseLl,
            _proc,
            NativeMethods.GetModuleHandleW(null),
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install low-level mouse hook.");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = (int)wParam;
            if (message == NativeMethods.WmLbuttondown)
            {
                IsLeftButtonDown = true;
                LeftButtonDown?.Invoke();
            }
            else if (message == NativeMethods.WmLbuttonup)
            {
                IsLeftButtonDown = false;
                LeftButtonUp?.Invoke();
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _proc = null;
    }
}
