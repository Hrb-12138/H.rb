namespace CnInstantTranslator.Translation;

public abstract class TranslationEngineBase : ITranslationEngine
{
    protected TranslationEngineBase(int cacheCapacity)
    {
        Cache = new TranslationCache(cacheCapacity);
    }

    public abstract string Name { get; }

    public TranslationCache Cache { get; }

    public async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (Cache.TryGet(text, targetLanguage, Name, out string cached))
        {
            return cached;
        }

        string translated = await TranslateCoreAsync(text, sourceLanguage, targetLanguage, cancellationToken);
        if (!string.IsNullOrWhiteSpace(translated))
        {
            Cache.Set(text, targetLanguage, Name, translated);
        }

        return translated;
    }

    protected abstract Task<string> TranslateCoreAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}
