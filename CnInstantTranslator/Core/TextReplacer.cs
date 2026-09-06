using System.Runtime.InteropServices;
using CnInstantTranslator.Native;

namespace CnInstantTranslator.Core;

/// <summary>
/// CapsLock 英文回填。
///
/// 使用 SendInput + KEYEVENTF_UNICODE，避免剪贴板污染；
/// 只在调用方已经验证“焦点未变且源文本仍然匹配”后执行，
/// 并通过 Ctrl+A 是否被接受来决定是否放弃。
/// </summary>
public sealed class TextReplacer
{
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    private readonly LowLevelKeyboardHook _keyboard;
    private readonly ImeStateProbe _ime;

    public TextReplacer(LowLevelKeyboardHook keyboard, ImeStateProbe ime)
    {
        _keyboard = keyboard;
        _ime = ime;
    }

    public Task<bool> ReplaceAsync(string english, IntPtr targetWindow, IntPtr focusedControl)
    {
        if (string.IsNullOrWhiteSpace(english))
        {
            DebugLog.Write("replacer abort: empty");
            return Task.FromResult(false);
        }

        if (NativeMethods.GetForegroundWindow() != targetWindow)
        {
            // 焦点已经不在原窗口：绝不能把英文发到别的窗口。
            DebugLog.Write("replacer abort: foreground mismatch");
            return Task.FromResult(false);
        }

        if (_keyboard.IsModifierDown || _ime.IsComposing(focusedControl))
        {
            DebugLog.Write("replacer abort: modifier/composing");
            return Task.FromResult(false);
        }

        uint currentThreadId = NativeMethods.GetCurrentThreadId();
        uint targetThreadId = NativeMethods.GetWindowThreadProcessId(targetWindow, IntPtr.Zero);
        bool attached = false;

        try
        {
            if (currentThreadId != targetThreadId && targetThreadId != 0)
            {
                attached = NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, true);
            }

            bool previousSuppress = _keyboard.SuppressActivity;
            _keyboard.SuppressActivity = true;
            try
            {
                NativeMethods.SetForegroundWindow(targetWindow);
                Thread.Sleep(20);

                if (!SendCtrlA())
                {
                    return Task.FromResult(false);
                }

                Thread.Sleep(28);
                SendVirtualKey(NativeMethods.VkDelete);
                Thread.Sleep(16);
                SendUnicode(english);
                return Task.FromResult(true);
            }
            finally
            {
                _keyboard.SuppressActivity = previousSuppress;
            }
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
    }

    private static bool SendCtrlA()
    {
        if (!SendVirtualKeyDown(NativeMethods.VkControl))
        {
            DebugLog.Write("replacer abort: ctrl down send failed");
            return false;
        }

        bool accepted = false;
        for (int i = 0; i < 12; i++)
        {
            if ((NativeMethods.GetAsyncKeyState(NativeMethods.VkControl) & 0x8000) != 0)
            {
                accepted = true;
                break;
            }

            Thread.Sleep(4);
        }

        if (!accepted)
        {
            SendVirtualKeyUp(NativeMethods.VkControl);
            DebugLog.Write("replacer abort: ctrl not accepted");
            return false;
        }

        bool aDown = SendVirtualKeyDown((byte)'A');
        bool aUp = SendVirtualKeyUp((byte)'A');
        bool ctrlUp = SendVirtualKeyUp(NativeMethods.VkControl);
        return aDown && aUp && ctrlUp;
    }

    private static void SendVirtualKey(int vk)
    {
        SendVirtualKeyDown(vk);
        SendVirtualKeyUp(vk);
    }

    private static bool SendVirtualKeyDown(int vk)
    {
        var input = new NativeMethods.INPUT
        {
            type = 1,
            U = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    dwFlags = 0
                }
            }
        };
        return SendInput(input);
    }

    private static bool SendVirtualKeyUp(int vk)
    {
        var input = new NativeMethods.INPUT
        {
            type = 1,
            U = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    dwFlags = KeyEventKeyUp
                }
            }
        };
        return SendInput(input);
    }

    private static bool SendUnicode(string text)
    {
        foreach (char ch in text)
        {
            var down = new NativeMethods.INPUT
            {
                type = 1,
                U = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)ch,
                        dwFlags = KeyEventUnicode
                    }
                }
            };

            var up = new NativeMethods.INPUT
            {
                type = 1,
                U = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)ch,
                        dwFlags = KeyEventUnicode | KeyEventKeyUp
                    }
                }
            };

            if (NativeMethods.SendInput(2, new[] { down, up }, Marshal.SizeOf<NativeMethods.INPUT>()) != 2)
            {
                DebugLog.Write($"replacer sendinput unicode failed at char {(ushort)ch} error={Marshal.GetLastWin32Error()}");
                return false;
            }
        }

        return true;
    }

    private static bool SendInput(NativeMethods.INPUT input)
    {
        uint sent = NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != 1)
        {
            DebugLog.Write($"replacer sendinput vk failed error={Marshal.GetLastWin32Error()}");
            return false;
        }

        return true;
    }
}
