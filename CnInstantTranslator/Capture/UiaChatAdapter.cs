using CnInstantTranslator.Domain;

namespace CnInstantTranslator.Capture;

/// <summary>
/// QQ / 标准 UIA 聊天框适配器。
///
/// 这条路径已经过验证，属于稳定路径。任何其它应用适配器都不得修改此文件；
/// 若后续需求被迫要改动这里，必须立即停止并重新设计方案。
/// </summary>
public sealed class UiaChatAdapter : ITextCaptureAdapter
{
    public CaptureKind Kind => CaptureKind.UiaChat;

    public Task<string?> GetTextAsync(FocusedContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ValuePattern 优先，TextPattern 的 GetSelection / GetVisibleRanges 不参与。
        string? text = UiaTextReader.TryReadFocusedValue()
                       ?? UiaTextReader.TryReadFromWindow(context.WindowHandle);

        return Task.FromResult(text);
    }
}
