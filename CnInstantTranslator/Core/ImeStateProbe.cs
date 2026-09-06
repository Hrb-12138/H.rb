using System.Runtime.InteropServices;
using CnInstantTranslator.Native;

namespace CnInstantTranslator.Core;

public sealed class ImeStateProbe
{
    private const int GcsCompStr = 0x0008;
    private const int GcsResultStr = 0x0800;

    public bool IsComposing(IntPtr windowHandle)
    {
        return ReadString(windowHandle, GcsCompStr, out _);
    }

    public bool TryReadComposition(IntPtr windowHandle, out string text)
    {
        return ReadString(windowHandle, GcsCompStr, out text);
    }

    public bool TryReadResult(IntPtr windowHandle, out string text)
    {
        return ReadString(windowHandle, GcsResultStr, out text);
    }

    private static bool ReadString(IntPtr windowHandle, int gcs, out string text)
    {
        text = string.Empty;
        IntPtr context = NativeMethods.ImmGetContext(windowHandle);
        if (context == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int length = NativeMethods.ImmGetCompositionStringW(context, gcs, IntPtr.Zero, 0);
            if (length <= 0)
            {
                return false;
            }

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NativeMethods.ImmGetCompositionStringW(context, gcs, buffer, length) <= 0)
                {
                    return false;
                }

                text = Marshal.PtrToStringUni(buffer, length / 2) ?? string.Empty;
                return !string.IsNullOrWhiteSpace(text);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.ImmReleaseContext(windowHandle, context);
        }
    }
}
