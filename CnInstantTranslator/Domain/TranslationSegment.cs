namespace CnInstantTranslator.Domain;

public sealed record TranslationSegment(int Index, string Text, string ContentHash);

public sealed record TranslationUpdate(
    string SourceText,
    IReadOnlyList<string?> Segments,
    string? FullTranslation,
    bool IsPartial,
    string Status);
