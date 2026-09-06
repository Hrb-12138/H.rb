using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CnInstantTranslator.Native;

namespace CnInstantTranslator.Core;

public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int KeyStateSize = 256;
    private IntPtr _hookHandle;
    private NativeMethods.LowLevelKeyboardProc? _proc;

    public event Action<KeyEvent>? KeyDown;
    public event Action<KeyEvent>? KeyUp;

    public bool SuppressActivity { get; set; }

    public bool IsModifierDown => IsKeyDown(NativeMethods.VkControl)
                                  || IsKeyDown(NativeMethods.VkShift)
                                  || IsKeyDown(NativeMethods.VkMenu);

    /// <summary>最近一次非修饰键输入的时间（用于限制盲剪贴板只在真实输入后触发）。</summary>
    public DateTime LastNonModifierActivityUtc { get; private set; } = DateTime.MinValue;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _proc = HookCallback;
        _hookHandle = NativeMethods.SetWindowsHookExW(
            NativeMethods.WhKeyboardLl,
            _proc,
            NativeMethods.GetModuleHandleW(null),
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install low-level keyboard hook.");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            int message = (int)wParam;
            bool isDown = message is NativeMethods.WmKeydown or NativeMethods.WmSyskeydown;
            bool isUp = message is NativeMethods.WmKeyup or NativeMethods.WmSyskeyup;

            if (isDown || isUp)
            {
                string text = isDown ? GetCharacter(data.vkCode, data.scanCode) : string.Empty;
                var keyEvent = new KeyEvent(
                    data.vkCode,
                    data.scanCode,
                    isDown,
                    (data.flags & 0x10) != 0,
                    text);

                bool isNonModifier = data.vkCode is not NativeMethods.VkControl
                    and not NativeMethods.VkShift
                    and not NativeMethods.VkMenu
                    and not NativeMethods.VkLwin
                    and not NativeMethods.VkRwin;

                if (isDown)
                {
                    if (!SuppressActivity && isNonModifier)
                    {
                        LastNonModifierActivityUtc = DateTime.UtcNow;
                    }

                    KeyDown?.Invoke(keyEvent);
                }
                else
                {
                    if (!SuppressActivity && isNonModifier)
                    {
                        LastNonModifierActivityUtc = DateTime.UtcNow;
                    }

                    KeyUp?.Invoke(keyEvent);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static string GetCharacter(uint virtualKey, uint scanCode)
    {
        var keyState = new byte[KeyStateSize];
        if (!NativeMethods.GetKeyboardState(keyState))
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(8);
        int length = NativeMethods.ToUnicode(virtualKey, scanCode, keyState, buffer, buffer.Capacity, 0);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    private static bool IsKeyDown(int vk)
    {
        return (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public void ReleaseStuckModifiers()
    {
        const uint keyUp = 0x0002;

        if (IsKeyDown(NativeMethods.VkControl))
        {
            NativeMethods.keybd_event(NativeMethods.VkControl, 0, keyUp, UIntPtr.Zero);
        }

        if (IsKeyDown(NativeMethods.VkShift))
        {
            NativeMethods.keybd_event(NativeMethods.VkShift, 0, keyUp, UIntPtr.Zero);
        }

        if (IsKeyDown(NativeMethods.VkMenu))
        {
            NativeMethods.keybd_event(NativeMethods.VkMenu, 0, keyUp, UIntPtr.Zero);
        }
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
