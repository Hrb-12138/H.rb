using CnInstantTranslator.Core;
using CnInstantTranslator.Domain;

namespace CnInstantTranslator.Capture;

public sealed class GenericAdapter : ITextCaptureAdapter
{
    private readonly InputMirrorService _mirror;
    private readonly BlindClipboardCapture _blindCapture;

    public GenericAdapter(InputMirrorService mirror, BlindClipboardCapture blindCapture)
    {
        _mirror = mirror;
        _blindCapture = blindCapture;
    }

    public CaptureKind Kind => CaptureKind.Generic;

    public Task<string?> GetTextAsync(FocusedContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? text = UiaTextReader.TryReadFocusedValue()
                       ?? UiaTextReader.TryReadFromWindow(context.WindowHandle);

        if (string.IsNullOrEmpty(text) && !_mirror.IsEmpty)
        {
            text = _mirror.CurrentText;
        }

        if (string.IsNullOrEmpty(text))
        {
            text = _blindCapture.TryCapture(context.WindowHandle, context.FocusedControlHandle, string.Empty);
        }

        if (!string.IsNullOrEmpty(text))
        {
            _mirror.SetFullText(text);
        }

        return Task.FromResult(text);
    }
}
