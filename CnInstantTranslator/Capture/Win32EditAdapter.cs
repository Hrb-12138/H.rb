using System.Text;
using CnInstantTranslator.Core;
using CnInstantTranslator.Domain;
using CnInstantTranslator.Native;

namespace CnInstantTranslator.Capture;

public sealed class Win32EditAdapter : ITextCaptureAdapter
{
    private readonly InputMirrorService _mirror;

    public Win32EditAdapter(InputMirrorService mirror)
    {
        _mirror = mirror;
    }

    public CaptureKind Kind => CaptureKind.Win32Edit;

    public Task<string?> GetTextAsync(FocusedContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? text = UiaTextReader.TryReadFocusedValue()
                       ?? UiaTextReader.TryReadFromWindow(context.WindowHandle);

        if (string.IsNullOrEmpty(text))
        {
            text = ReadViaWindowMessage(context.FocusedControlHandle);
        }

        if (!string.IsNullOrEmpty(text))
        {
            _mirror.SetFullText(text);
            return Task.FromResult<string?>(text);
        }

        string mirrorText = _mirror.CurrentText;
        return Task.FromResult<string?>(string.IsNullOrEmpty(mirrorText) ? null : mirrorText);
    }

    private static string? ReadViaWindowMessage(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        int length = NativeMethods.SendMessageW(hwnd, NativeMethods.WmGettextlength, IntPtr.Zero, IntPtr.Zero);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new StringBuilder(length + 1);
        NativeMethods.SendMessageW(hwnd, NativeMethods.WmGettext, new IntPtr(buffer.Capacity), buffer);
        return buffer.ToString();
    }
}
