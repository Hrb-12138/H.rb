namespace CnInstantTranslator.Translation;

public interface ITranslationEngine
{
    string Name { get; }

    TranslationCache Cache { get; }

    Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}
