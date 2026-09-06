using CnInstantTranslator.Domain;

namespace CnInstantTranslator.Capture;

public interface ITextCaptureAdapter
{
    CaptureKind Kind { get; }

    Task<string?> GetTextAsync(FocusedContext context, CancellationToken cancellationToken);
}
