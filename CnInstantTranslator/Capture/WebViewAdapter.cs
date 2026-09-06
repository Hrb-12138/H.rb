using CnInstantTranslator.Core;
using CnInstantTranslator.Domain;

namespace CnInstantTranslator.Capture;

public sealed class WebViewAdapter : ITextCaptureAdapter
{
    private readonly InputMirrorService _mirror;

    public WebViewAdapter(InputMirrorService mirror)
    {
        _mirror = mirror;
    }

    public CaptureKind Kind => CaptureKind.WebView;

    public Task<string?> GetTextAsync(FocusedContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? text = UiaTextReader.TryReadFocusedValue()
                       ?? UiaTextReader.TryReadFromWindow(context.WindowHandle);

        if (!string.IsNullOrEmpty(text))
        {
            _mirror.SetFullText(text);
            return Task.FromResult<string?>(text);
        }

        string mirrorText = _mirror.CurrentText;
        return Task.FromResult<string?>(string.IsNullOrEmpty(mirrorText) ? null : mirrorText);
    }
}
