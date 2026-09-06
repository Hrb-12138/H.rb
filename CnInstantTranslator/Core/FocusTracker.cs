using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CnInstantTranslator.Domain;
using CnInstantTranslator.Native;

namespace CnInstantTranslator.Core;

public sealed class FocusTracker
{
    private readonly AppCaptureClassifier _classifier;

    public FocusTracker(AppCaptureClassifier classifier)
    {
        _classifier = classifier;
    }

    public FocusedContext GetCurrent()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return FocusedContext.Empty;
        }

        uint processId = 0;
        uint threadId = NativeMethods.GetWindowThreadProcessId(foreground, out processId);

        IntPtr focusedControl = GetFocusedControl(threadId);
        if (focusedControl == IntPtr.Zero)
        {
            focusedControl = foreground;
        }

        string processName = TryGetProcessName(processId);
        string windowClass = GetWindowClassName(foreground);
        string windowTitle = GetWindowText(foreground);

        var context = new FocusedContext(
            foreground,
            focusedControl,
            processId,
            processName,
            windowClass,
            windowTitle,
            CaptureKind.Unknown);

        return context with { Kind = _classifier.Classify(context) };
    }

    private static IntPtr GetFocusedControl(uint threadId)
    {
        if (threadId == 0)
        {
            return NativeMethods.GetFocus();
        }

        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        if (NativeMethods.GetGUIThreadInfo(threadId, ref info))
        {
            return info.hwndFocus;
        }

        return NativeMethods.GetFocus();
    }

    private static string TryGetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return string.Empty;
        }
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        return NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var buffer = new StringBuilder(1024);
        return NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }
}
