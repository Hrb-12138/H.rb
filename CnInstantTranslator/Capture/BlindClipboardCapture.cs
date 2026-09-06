using CnInstantTranslator.Core;
using CnInstantTranslator.Native;

namespace CnInstantTranslator.Capture;

/// <summary>
/// 门禁式盲剪贴板捕获。
///
/// 与原实现相比新增了：
/// 1. 只有最近确实发生过真实键盘/粘贴输入时才允许执行，避免在只读窗口或
///    空输入框上反复 Ctrl+A/C；
/// 2. 捕获前写入唯一标记，捕获结果必须与标记不同，避免“Ctrl 被吞但读到旧内容”；
/// 3. 连续失败进入 poison 退避；
/// 4. 原剪贴板为空时也会清理，不再把用户输入遗留在剪贴板。
/// </summary>
public sealed class BlindClipboardCapture
{
    private static readonly TimeSpan RecentInputWindow = TimeSpan.FromSeconds(10);

    private readonly InputMirrorService _mirror;
    private readonly IdleOrSpaceTrigger _idle;
    private readonly LowLevelKeyboardHook _keyboard;
    private readonly LowLevelMouseHook _mouse;
    private readonly ImeStateProbe _ime;
    private readonly int _cooldownMs;
    private readonly Func<bool> _allowCapture;

    private long _lastCaptureTicks;
    private DateTime _poisonUntilUtc = DateTime.MinValue;
    private int _busy;
    private int _consecutiveFails;

    public BlindClipboardCapture(
        InputMirrorService mirror,
        IdleOrSpaceTrigger idle,
        LowLevelKeyboardHook keyboard,
        LowLevelMouseHook mouse,
        ImeStateProbe ime,
        int cooldownMs,
        Func<bool>? allowCapture = null)
    {
        _mirror = mirror;
        _idle = idle;
        _keyboard = keyboard;
        _mouse = mouse;
        _ime = ime;
        _cooldownMs = Math.Max(250, cooldownMs);
        _allowCapture = allowCapture ?? (() => true);
    }

    public string? TryCapture(IntPtr targetWindow, IntPtr focusedControl, string previousText)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return null;
        }

        try
        {
            if (!CanCaptureNow(focusedControl))
            {
                return null;
            }

            // 阅读/空等场景不执行：只有近期发生过真实输入才允许盲剪贴板。
            if (!_allowCapture())
            {
                return null;
            }

            Interlocked.Exchange(ref _lastCaptureTicks, DateTime.UtcNow.Ticks);

            ClipboardSnapshot? original = SaveClipboard();
            if (original is null)
            {
                // 无法读取原剪贴板（可能被其它进程占用），放弃本次捕获，
                // 避免后续把剪贴板数据改坏。
                return null;
            }

            string marker = "\u200b" + Guid.NewGuid().ToString("N") + "\u200b";
            bool markerSet = false;
            bool sentKeys = false;
            uint currentThreadId = NativeMethods.GetCurrentThreadId();
            uint targetThreadId = NativeMethods.GetWindowThreadProcessId(targetWindow, IntPtr.Zero);
            bool attached = false;

            try
            {
                if (currentThreadId != targetThreadId && targetThreadId != 0)
                {
                    attached = NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, true);
                }

                NativeMethods.SetForegroundWindow(targetWindow);
                if (!CanContinueCapture(focusedControl))
                {
                    return null;
                }

                if (!TrySetClipboardText(marker))
                {
                    return null;
                }

                markerSet = true;

                _keyboard.SuppressActivity = true;
                try
                {
                    if (!SendCtrlChord((byte)'A'))
                    {
                        NoteFailure(TimeSpan.FromSeconds(4));
                        return null;
                    }

                    sentKeys = true;
                    if (AbortableSleep(35, focusedControl) is false)
                    {
                        return null;
                    }

                    if (!CanContinueCapture(focusedControl))
                    {
                        return null;
                    }

                    if (!SendCtrlChord((byte)'C'))
                    {
                        NoteFailure(TimeSpan.FromSeconds(4));
                        return null;
                    }

                    if (AbortableSleep(35, focusedControl) is false)
                    {
                        return null;
                    }

                    if (!CanContinueCapture(focusedControl))
                    {
                        return null;
                    }

                    // 收起选区，降低对用户当前操作的干扰。
                    SendKey(NativeMethods.VkEnd);
                }
                finally
                {
                    _keyboard.SuppressActivity = false;
                }

                string? text = ReadClipboardText();
                if (string.IsNullOrWhiteSpace(text)
                    || text == marker
                    || (previousText.Length > 0 && text == previousText))
                {
                    NoteFailure();
                    return null;
                }

                text = text.Trim('\r', '\n', ' ');
                if (text.Length == 0
                    || text.Length > 1200
                    || !TextUtil.HasChinese(text))
                {
                    NoteFailure();
                    return null;
                }

                _consecutiveFails = 0;
                return text;
            }
            finally
            {
                if (sentKeys)
                {
                    // 确保把光标移回结尾，避免把聊天框中的选中状态留给用户。
                    try
                    {
                        SendKey(NativeMethods.VkEnd);
                    }
                    catch
                    {
                        // 已尽力恢复光标状态。
                    }
                }

                if (attached)
                {
                    NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, false);
                }

                if (markerSet || original.Data is not null)
                {
                    RestoreClipboard(original);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private bool CanCaptureNow(IntPtr focusedControl)
    {
        if (!CanContinueCapture(focusedControl))
        {
            return false;
        }

        if (DateTime.UtcNow < _poisonUntilUtc)
        {
            return false;
        }

        long elapsed = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastCaptureTicks)) / TimeSpan.TicksPerMillisecond;
        return elapsed >= _cooldownMs;
    }

    private bool CanContinueCapture(IntPtr focusedControl)
    {
        if (!_idle.IsIdle)
        {
            return false;
        }

        if (_keyboard.IsModifierDown)
        {
            return false;
        }

        if (_mouse.IsLeftButtonDown)
        {
            return false;
        }

        if (_ime.IsComposing(focusedControl))
        {
            return false;
        }

        return true;
    }

    private void NoteFailure(TimeSpan? poison = null)
    {
        _consecutiveFails++;
        _poisonUntilUtc = DateTime.UtcNow + (poison
            ?? (_consecutiveFails >= 2 ? TimeSpan.FromSeconds(2.5) : TimeSpan.FromMilliseconds(800)));
    }

    private static bool SendCtrlChord(byte vk)
    {
        NativeMethods.keybd_event((byte)NativeMethods.VkControl, 0, 0, UIntPtr.Zero);
        bool ctrlAccepted = false;
        for (int i = 0; i < 12; i++)
        {
            if ((NativeMethods.GetAsyncKeyState(NativeMethods.VkControl) & 0x8000) != 0)
            {
                ctrlAccepted = true;
                break;
            }

            Thread.Sleep(4);
        }

        if (!ctrlAccepted)
        {
            NativeMethods.keybd_event((byte)NativeMethods.VkControl, 0, 0x0002, UIntPtr.Zero);
            return false;
        }

        NativeMethods.keybd_event(vk, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(vk, 0, 0x0002, UIntPtr.Zero);
        NativeMethods.keybd_event((byte)NativeMethods.VkControl, 0, 0x0002, UIntPtr.Zero);
        return true;
    }

    private static void SendKey(byte vk)
    {
        NativeMethods.keybd_event(vk, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(vk, 0, 0x0002, UIntPtr.Zero);
    }

    private bool AbortableSleep(int milliseconds, IntPtr focusedControl)
    {
        int remaining = milliseconds;
        while (remaining > 0)
        {
            if (!CanContinueCapture(focusedControl))
            {
                return false;
            }

            int step = Math.Min(8, remaining);
            Thread.Sleep(step);
            remaining -= step;
        }

        return CanContinueCapture(focusedControl);
    }

    private static bool TrySetClipboardText(string text)
    {
        try
        {
            System.Windows.Forms.Clipboard.SetText(text, System.Windows.Forms.TextDataFormat.UnicodeText);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ClipboardSnapshot? SaveClipboard()
    {
        try
        {
            IDataObject? data = System.Windows.Forms.Clipboard.GetDataObject();
            return new ClipboardSnapshot(data);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadClipboardText()
    {
        try
        {
            return System.Windows.Forms.Clipboard.ContainsText()
                ? System.Windows.Forms.Clipboard.GetText()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void RestoreClipboard(ClipboardSnapshot original)
    {
        try
        {
            if (original.Data is not null)
            {
                System.Windows.Forms.Clipboard.SetDataObject(original.Data, true);
            }
            else
            {
                System.Windows.Forms.Clipboard.Clear();
            }
        }
        catch
        {
            // 恢复失败时无法回滚，但不影响本次捕获结果。
        }
    }

    private sealed record ClipboardSnapshot(IDataObject? Data);
}
