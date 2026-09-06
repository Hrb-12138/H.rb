using CnInstantTranslator.Domain;

namespace CnInstantTranslator.Capture;

public sealed class DocumentAdapter : ITextCaptureAdapter
{
    public CaptureKind Kind => CaptureKind.Document;

    public Task<string?> GetTextAsync(FocusedContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Word/WPS 在未安装对应 COM Interop 时先走 UIA。
        // TextPattern 仅作为文档场景的最后兜底，且调用方需确保键盘/鼠标安静。
        string? text = UiaTextReader.TryReadFocusedValue()
                       ?? UiaTextReader.TryReadFromWindow(context.WindowHandle)
                       ?? UiaTextReader.TryReadTextPatternSelection(context.WindowHandle);

        return Task.FromResult(text);
    }
}
