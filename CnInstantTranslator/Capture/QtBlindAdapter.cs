using CnInstantTranslator.Core;
using CnInstantTranslator.Domain;

namespace CnInstantTranslator.Capture;

public sealed class QtBlindAdapter : ITextCaptureAdapter
{
    private readonly InputMirrorService _mirror;
    private readonly BlindClipboardCapture _blindCapture;
    private readonly ImeStateProbe _ime;

    public QtBlindAdapter(InputMirrorService mirror, BlindClipboardCapture blindCapture, ImeStateProbe ime)
    {
        _mirror = mirror;
        _blindCapture = blindCapture;
        _ime = ime;
    }

    public CaptureKind Kind => CaptureKind.QtBlind;

    public Task<string?> GetTextAsync(FocusedContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 先尝试 IMM 最新文本，避免旧镜像把后面输入卡死。
        string? immText = _mirror.PullImmSnapshot(context.WindowHandle, context.FocusedControlHandle, _ime, returnCurrentWhenEmpty: false);
        if (!string.IsNullOrEmpty(immText) && TextUtil.HasChinese(immText))
        {
            _mirror.SetFullText(immText);
            return Task.FromResult<string?>(immText);
        }

        string mirrorText = _mirror.CurrentText;
        if (!string.IsNullOrEmpty(mirrorText) && DateTime.UtcNow - _mirror.LastFullTextAt < TimeSpan.FromSeconds(2.5))
        {
            return Task.FromResult<string?>(mirrorText);
        }

        string? captured = _blindCapture.TryCapture(
            context.WindowHandle,
            context.FocusedControlHandle,
            mirrorText);

        if (!string.IsNullOrEmpty(captured))
        {
            _mirror.SetFullText(captured);
        }

        return Task.FromResult(captured);
    }
}
