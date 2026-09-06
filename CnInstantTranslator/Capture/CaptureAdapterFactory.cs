using CnInstantTranslator.Core;
using CnInstantTranslator.Domain;

namespace CnInstantTranslator.Capture;

public sealed class CaptureAdapterFactory
{
    private readonly InputMirrorService _mirror;
    private readonly BlindClipboardCapture _blindCapture;
    private readonly ImeStateProbe _ime;

    public CaptureAdapterFactory(InputMirrorService mirror, BlindClipboardCapture blindCapture, ImeStateProbe ime)
    {
        _mirror = mirror;
        _blindCapture = blindCapture;
        _ime = ime;
    }

    public ITextCaptureAdapter Create(CaptureKind kind)
    {
        return kind switch
        {
            CaptureKind.UiaChat => new UiaChatAdapter(),
            CaptureKind.QtBlind => new QtBlindAdapter(_mirror, _blindCapture, _ime),
            CaptureKind.WebView => new WebViewAdapter(_mirror),
            CaptureKind.Win32Edit => new Win32EditAdapter(_mirror),
            CaptureKind.Document => new DocumentAdapter(),
            CaptureKind.Generic => new GenericAdapter(_mirror, _blindCapture),
            _ => new GenericAdapter(_mirror, _blindCapture)
        };
    }
}
