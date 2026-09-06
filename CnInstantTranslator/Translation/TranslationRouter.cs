namespace CnInstantTranslator.Translation;

public sealed class TranslationRouter
{
    private readonly IReadOnlyList<ITranslationEngine> _engines;
    private readonly int _maxEngineAttempts;
    private readonly TimeSpan _timeout;

    public TranslationRouter(
        IEnumerable<ITranslationEngine> engines,
        int maxEngineAttempts = 2,
        TimeSpan? timeout = null)
    {
        _engines = engines.ToArray();
        _maxEngineAttempts = Math.Max(1, maxEngineAttempts);
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<TranslationAttempt?> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || _engines.Count == 0)
        {
            return null;
        }

        int attempts = 0;
        Exception? lastError = null;

        foreach (ITranslationEngine engine in _engines)
        {
            if (attempts >= _maxEngineAttempts)
            {
                break;
            }

            attempts++;

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);

            try
            {
                string translated = await engine.TranslateAsync(
                    text,
                    sourceLanguage,
                    targetLanguage,
                    timeoutSource.Token);

                if (!string.IsNullOrWhiteSpace(translated))
                {
                    return new TranslationAttempt(translated, engine.Name);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException($"Engine '{engine.Name}' timed out after {_timeout.TotalSeconds:0.#}s.");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            System.Diagnostics.Debug.WriteLine(lastError.ToString());
        }

        return null;
    }
}
