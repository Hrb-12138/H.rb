using System.Diagnostics;

namespace CnInstantTranslator.Domain;

public sealed record FocusedContext(
    IntPtr WindowHandle,
    IntPtr FocusedControlHandle,
    uint ProcessId,
    string ProcessName,
    string WindowClassName,
    string WindowTitle,
    CaptureKind Kind)
{
    public static FocusedContext Empty { get; } = new(
        IntPtr.Zero,
        IntPtr.Zero,
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        CaptureKind.Unknown);

    public bool IsSameWeChatSession(FocusedContext other)
    {
        if (Kind != CaptureKind.QtBlind || other.Kind != CaptureKind.QtBlind)
        {
            return false;
        }

        return WindowHandle == other.WindowHandle
               && ProcessId == other.ProcessId
               && string.Equals(WindowTitle, other.WindowTitle, StringComparison.Ordinal);
    }
}
